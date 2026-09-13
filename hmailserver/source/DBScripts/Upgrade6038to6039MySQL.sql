alter table hm_domains modify domainrelaypassword varchar(1024) not null;

update hm_dbversion set value = 6039;
