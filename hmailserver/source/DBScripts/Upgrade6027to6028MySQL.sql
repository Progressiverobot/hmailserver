create table hm_metricsamples
(
	metricsampleid int auto_increment not null, primary key(`metricsampleid`), unique(`metricsampleid`),
	metricsampletime datetime not null,
	metricsamplename varchar(64) not null,
	metricsamplevalue double not null
) DEFAULT CHARSET=utf8;

CREATE INDEX idx_hm_metricsamples_name_time ON hm_metricsamples (metricsamplename, metricsampletime);

update hm_dbversion set value = 6028;
