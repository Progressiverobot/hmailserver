create table if not exists hm_inisettings
(
	inisettingid int auto_increment not null, primary key(`inisettingid`), unique(`inisettingid`),
	inisettingname varchar (100) not null, unique(`inisettingname`),
	inisettingvalue text not null,
	inisettingfilevalue text not null
) DEFAULT CHARSET=utf8;

update hm_dbversion set value = 6011;
