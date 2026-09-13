alter table hm_domains alter column domainrelaypassword type varchar(1024);

update hm_dbversion set value = 6039;
